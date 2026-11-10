using System;
using System.Runtime.InteropServices;

namespace ForgeOrdnanceFrames;

/// <summary>Host to every peer, through ForgeOrdnance's kept per-call state (<c>OrdnanceApi.SendState</c>, tag
/// <see cref="FrameMessages.StateTag"/>, replayed to players who join later): the whole record of one suit after each change. The call (thrower, throw id) the
/// data travels with identifies the suit.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FrameStateMessage
{
    public uint Sequence;
    public byte State;
    public byte Flags;
    public ulong Pilot;
    public float Armor;
    public float Shield;
    public int Magazine;
    public int Reserve;
    public float X, Y, Z, Yaw;
    /// <summary>Seconds until the timed state ends, on the sender's clock (each peer adds its own time).</summary>
    public float StateRemaining;
    public float BoostRemaining;
    /// <summary>Why the pilot left without asking (<see cref="FrameEjection"/>), 0 otherwise.</summary>
    public byte Ejection;
    /// <summary>The suit's model (<see cref="FrameSpecKey"/>), so a player who joined later can build the suit from this alone.</summary>
    public ulong Spec;
    /// <summary>The definition the suit was called from (<see cref="FrameRecord.DefinitionKey"/>).</summary>
    public uint Definition;
    public ulong Authority;
    public float BoostCooldownRemaining, RocketsCooldownRemaining, ShieldRegenRemaining, SiegeDeployRemaining;
    /// <summary>The boarding the suit is in (<see cref="FrameRecord.Drive"/>): a request names the one it was made in.</summary>
    public uint Drive;

    public const byte FlagSieged = 1;
    public const byte FlagShieldUp = 2;

    public static FrameStateMessage From(FrameRecord f, float now, FrameEjection ejection) => new()
    {
        Sequence = f.Sequence,
        State = (byte)f.State,
        Flags = (byte)((f.Sieged ? FlagSieged : 0) | (f.ShieldUp ? FlagShieldUp : 0)),
        Pilot = f.Pilot,
        Armor = f.Armor,
        Shield = f.Shield,
        Magazine = f.Magazine,
        Reserve = f.Reserve,
        X = f.X,
        Y = f.Y,
        Z = f.Z,
        Yaw = f.Yaw,
        StateRemaining = Math.Max(0f, f.StateEndsAt - now),
        BoostRemaining = Math.Max(0f, f.BoostEndsAt - now),
        Ejection = (byte)ejection,
        Spec = f.SpecKey,
        Definition = f.DefinitionKey,
        Authority = f.Authority,
        BoostCooldownRemaining = Math.Max(0f, f.BoostReadyAt - now),
        RocketsCooldownRemaining = Math.Max(0f, f.RocketsReadyAt - now),
        ShieldRegenRemaining = Math.Max(0f, (f.Spec.Shield?.RegenDelay ?? 0f) - (now - f.ShieldLastHit)),
        SiegeDeployRemaining = Math.Max(0f, f.SiegeDeployEndsAt - now),
        Drive = f.Drive,
    };

    /// <summary>Copies the message into a mirror record. Returns false for a stale or repeated sequence.</summary>
    public bool ApplyTo(FrameRecord f, float now)
    {
        if (Authority == f.Authority && unchecked((int)(Sequence - f.Sequence)) <= 0 && f.Sequence != 0) return false;
        if (!Enum.IsDefined(typeof(FrameState), State)) return false;
        f.Sequence = Sequence; f.State = (FrameState)State; f.Sieged = (Flags & FlagSieged) != 0; f.ShieldUp = (Flags & FlagShieldUp) != 0; f.Pilot = Pilot;
        f.Armor = Armor; f.Shield = Shield; f.Magazine = Magazine; f.Reserve = Reserve; f.X = X; f.Y = Y; f.Z = Z; f.Yaw = Yaw;
        f.StateEndsAt = now + StateRemaining; f.BoostEndsAt = BoostRemaining > 0f ? now + BoostRemaining : 0f;
        f.Authority = Authority; f.DefinitionKey = Definition; f.Drive = Drive;
        f.BoostReadyAt = now + BoostCooldownRemaining; f.RocketsReadyAt = now + RocketsCooldownRemaining;
        f.ShieldLastHit = now + ShieldRegenRemaining - (f.Spec.Shield?.RegenDelay ?? 0f);
        f.SiegeDeployEndsAt = SiegeDeployRemaining > 0f ? now + SiegeDeployRemaining : 0f;
        return true;
    }
}

/// <summary>Any peer to the host (<see cref="FrameMessages.RequestEvent"/>): board, leave, report ammunition, use a special. It carries the
/// world epoch it was made in (a request from before a checkpoint reload or level change is dropped) and, but for boarding, the boarding
/// it belongs to (<see cref="FrameRecord.Drive"/>). A mode change names its target (<see cref="Value"/>), never "toggle".</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FrameRequestMessage
{
    public ulong Thrower;
    public uint ThrowId;
    public byte Kind;
    public int Magazine;
    public int Reserve;
    public ulong Epoch;
    public uint Drive;
    /// <summary>Siege: 1 to plant, 0 to stand down.</summary>
    public byte Value;
}

/// <summary>Host to the requester (<see cref="FrameMessages.RejectEvent"/>): why a request was refused.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FrameRejectMessage
{
    public ulong Thrower;
    public uint ThrowId;
    public byte Kind;
    public byte Reason;
}

/// <summary>Pilot to every peer (<see cref="FrameMessages.BumpEvent"/>): the charge's shoulder hit a door or an enemy at this point, moving
/// along (<c>DirX</c>, <c>DirZ</c>): the other peers play the same shoulder-bash and impact. Presentation only.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FrameBumpMessage
{
    public ulong Thrower;
    public uint ThrowId;
    public float X, Y, Z, DirX, DirZ;
}

/// <summary>Pilot to every peer (<see cref="FrameMessages.FireEvent"/>): shots fired this frame, for the sound and tracer on
/// other peers. Damage is not in it: the pilot's own game deals it through the game's bullet hit, which the game replicates.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FrameFireMessage
{
    public ulong Thrower;
    public uint ThrowId;
    public float OriginX, OriginY, OriginZ;
    public float TargetX, TargetY, TargetZ;
    public byte Shots;
}

/// <summary>Rockets. Pilot to host: the aimed point (<see cref="Seed"/> unused). Host to everyone: a granted salvo, its seed
/// fixing every rocket's scatter.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FrameRocketMessage
{
    public ulong Thrower;
    public uint ThrowId;
    public float TargetX, TargetY, TargetZ;
    public ushort Seed;
    /// <summary>The world epoch and boarding the message was made in; the receiver drops one that is not its current.</summary>
    public ulong Epoch;
    public uint Drive;
}

/// <summary>Host to the pilot (<see cref="FrameMessages.HitEvent"/>): a hit the suit took, with where it came from, for the pilot's
/// hit-direction flash and damage outline. Presentation only; the armour travels in the state.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FrameHitMessage
{
    public ulong Thrower;
    public uint ThrowId;
    /// <summary>The attacker's position on the ground plane; NaN for a hit with no attacker (a fall, the level).</summary>
    public float SourceX, SourceZ;
    /// <summary>The armour the hit took (after the suit's reduction), or the barrier's when <see cref="Shielded"/>.</summary>
    public float Damage;
    public byte Shielded;
}

/// <summary>Reliable checkpoint transaction, independent of the framework's retained live states.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FrameCheckpointMessage
{
    public const byte Request = 0, Begin = 1, Entry = 2, Commit = 3;
    public byte Kind;
    public int Count;
    public uint Revision;
    public ulong Thrower;
    public uint ThrowId;
    public FrameStateMessage State;
}

internal static class FrameMessages
{
    /// <summary>Message names on this plugin's ForgeCore channel (ForgeCore adds the plugin; names, delivery and layouts are fingerprinted).</summary>
    public const string RequestEvent = "Request";
    public const string RejectEvent = "Reject";
    public const string FireEvent = "Fire";
    public const string BumpEvent = "Bump";
    public const string RocketRequestEvent = "RocketRequest";
    public const string RocketLaunchEvent = "RocketLaunch";
    public const string CheckpointEvent = "Checkpoint";
    public const string HitEvent = "Hit";
    public const byte StateTag = 1;

    public static unsafe byte[] ToBytes<T>(T message) where T : unmanaged
    {
        var bytes = new byte[sizeof(T)];
        MemoryMarshal.Write(bytes, ref message);
        return bytes;
    }

    public static unsafe bool TryRead<T>(byte[] bytes, out T message) where T : unmanaged
    {
        message = default;
        if (bytes == null || bytes.Length != sizeof(T)) return false;
        message = MemoryMarshal.Read<T>(bytes);
        return true;
    }
}
