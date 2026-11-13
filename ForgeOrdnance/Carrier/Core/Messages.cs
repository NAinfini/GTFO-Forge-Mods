using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ForgeOrdnance;

/// <summary>Why a call was refused. The value travels as the one-byte answer of the loadout and landing requests.</summary>
internal enum Rejection : byte
{
    None = 0,
    NotInLoadout = 1,
    Cooldown = 2,
    NoUses = 3,
    TooFar = 4,
    InvalidLanding = 5,
    // 6 to 8 are not used.
    UnknownDefinition = 9,
    LoadoutInvalid = 10,
    LandingTimeout = 11,
    CallMenuOff = 12,
    /// <summary>The host gave no answer in time, or left before it answered.</summary>
    NoAnswer = 13
}

internal static class Messages
{
    /// <summary>Fixed on every peer; native pReplicationData.PrefabID is only 16 bits.</summary>
    public const ushort BeaconItemId = 65114;
    public const int MaxLoadout = 8;
    /// <summary>Lobby values (ForgeCore per-player lobby data): each player's picks, and the host's rules for the selected expedition.</summary>
    public const string LobbyPicks = "picks", LobbyRules = "rules";
    /// <summary>The most bytes a payload type sends about one call in one message or keeps in one state.</summary>
    public const int MaxPayloadData = 192;
    /// <summary>The content parts' own ids under the mod's: each names its part's sound set in ForgeAudio and its lease on ForgeCore's special slot.</summary>
    public const string ArsenalId = "NAinfini.ForgeOrdnance.Arsenal", FramesId = "NAinfini.ForgeOrdnance.Frames";
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Vec3
{
    public float X, Y, Z;
    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public readonly bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
    public static float Distance(Vec3 a, Vec3 b)
    {
        float x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
        return MathF.Sqrt(x * x + y * y + z * z);
    }
}

/// <summary>One call on every peer: its thrower (0 for a developer drop) and that thrower's number for it.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly record struct CallKey(ulong Thrower, uint ThrowId);

/// <summary>One call-in of one player.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly record struct SlotKey(ulong Player, uint Key);

/// <summary>One kept state of one call: the FNV-1a hash of its payload type name, the call, and the payload's own tag.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly record struct StateKey(uint Type, ulong Thrower, uint ThrowId, byte Tag);

/// <summary>A loadout: the body of a client's submission to the host, and the host's row for the accepted loadout of one player.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct LoadoutRow
{
    public byte Count;
    public fixed uint Keys[Messages.MaxLoadout];

    public static LoadoutRow From(IReadOnlyList<uint> keys)
    {
        if (keys.Count > Messages.MaxLoadout) throw new ArgumentException("Loadout is longer than " + Messages.MaxLoadout + ".", nameof(keys));
        var row = new LoadoutRow { Count = (byte)keys.Count };
        for (var i = 0; i < keys.Count; i++) row.Keys[i] = keys[i];
        return row;
    }

    /// <summary>The keys, or null when <see cref="Count"/> exceeds the fixed capacity (a malformed message).</summary>
    public readonly uint[]? ToKeys()
    {
        if (Count > Messages.MaxLoadout) return null;
        var keys = new uint[Count];
        fixed (uint* source = Keys) for (var i = 0; i < Count; i++) keys[i] = source[i];
        return keys;
    }
}

/// <summary>The host's row for one slot of one player, written after every change.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SlotRow
{
    /// <summary>Negative: the call-in has no use limit.</summary>
    public int UsesLeft;
    /// <summary>Seconds, counted from when the host wrote the row.</summary>
    public float CooldownRemaining;
    /// <summary>The uses the slot started with: a peer that sees the slot only now (it joined late) cannot tell them from the uses already spent.</summary>
    public int InitialUses;
    /// <summary>Counts the host's writes of this row: the same numbers written again (the same cooldown started again) are a change.</summary>
    public uint Write;
}

/// <summary>The thrower's report that the ball came to rest: the body of the landing request. The host answers with a <see cref="Rejection"/>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct LandingMessage
{
    public uint ThrowId;
    public uint Key;
    public ushort Replicator;
    public Vec3 Origin;
    public Vec3 Landing;
}

/// <summary>The host's row for a call it accepted and has not delivered yet: the beacon stands at <see cref="Landing"/> and the
/// arrival follows after <see cref="Delay"/> seconds, counted from when the host wrote the row.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct CallRow
{
    public uint Key;
    public Vec3 Landing;
    public float Delay;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ArrivedMessage
{
    public ulong Thrower;
    public uint ThrowId;
    public uint Key;
    public Vec3 Landing;
    /// <summary>Degrees about the vertical: the host's arrival rotation, so every peer orients the drop pod the same way.</summary>
    public float Yaw;
    /// <summary><see cref="OrdnanceArrivalMode"/>: with or without the vehicle.</summary>
    public byte Arrival;
}

/// <summary>The drop pod or the turret shell of one call.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly record struct DeliveryKey(ulong Thrower, uint ThrowId, byte Kind)
{
    public const byte Pod = 1;
    public const byte Shell = 2;
}

/// <summary>The host's row for a drop pod or turret shell standing in the level, so a peer that did not see it arrive shows it in
/// its current state. <see cref="Elapsed"/> is seconds since the arrival started on the host; <see cref="SinceEmptied"/> seconds since
/// the pod's last pickup was taken or the shell's sentry went, or negative. Both are counted to when the host wrote the row.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DeliveryRow
{
    public Vec3 Landing;
    public float Yaw;
    public float Elapsed;
    public float SinceEmptied;
    /// <summary>The call-in's definition: its payload gives a pod's or shell's appearance, so a joiner draws what everyone else does.</summary>
    public uint DefinitionKey;
}

/// <summary>The host's row for a summoned sentry that ends by itself; the row's key is the native gear's replicator key-plus-one.
/// <see cref="Identity"/> is its exact per-spawn identity, <see cref="Remaining"/> the seconds until removal, counted from when the
/// host wrote the row.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SentryRow
{
    public ulong Identity;
    public float Remaining;
}

/// <summary>Client to host: a step of taking a claimable (<c>OrdnancePod.AddClaimable</c>). <see cref="Attempt"/> is the claimant's
/// own sequence number; later steps and the host's answers match it.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ClaimMessage
{
    /// <summary>Ask for it; the gear is fully installed; installing failed or timed out.</summary>
    public const byte Request = 0, Installed = 1, Failed = 2;
    public ulong Thrower;
    public uint ThrowId;
    public uint Attempt;
    public byte Action;
    /// <summary>Which weapon bay of the pod (0 for a claimable lying without a pod).</summary>
    public byte Bay;
}

/// <summary>Host to every peer: <see cref="Reserved"/> while the claimant installs it, <see cref="Committed"/> when it is taken (with
/// the claiming package's own <see cref="Token"/>), <see cref="Cancelled"/> when it is open again.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ClaimStateMessage
{
    public const byte Reserved = 0, Committed = 1, Cancelled = 2;
    public ulong Thrower;
    public uint ThrowId;
    public ulong Player;
    public uint Attempt;
    public byte State;
    public Guid Token;
    public byte Bay;
}
